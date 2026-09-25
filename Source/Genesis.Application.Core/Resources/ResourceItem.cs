namespace Genesis.Application.Core.Resources;

public sealed class ResourceItem
{
    public required string Name { get; set; }

    public required string FullPath { get; init; }

    public required string RelativePath { get; init; }

    public required ResourceKind Kind { get; init; }

    public required bool IsFolder { get; init; }

    public bool IsProtectedRoot { get; init; }

    public ResourceKind? AllowedResourceKind { get; init; }

    public Guid AssetId { get; init; }

    /// <summary>Editor library classification, separate from animation/gameplay tags.</summary>
    public IReadOnlyList<string> LibraryTags { get; init; } = [];

    public List<ResourceItem> Children { get; init; } = [];
}

public sealed class AssetMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required string Guid { get; set; }

    public required string Kind { get; set; }

    public required string DisplayName { get; set; }

    public string? ResourceName { get; set; }

    public List<string> LibraryTags { get; set; } = [];

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}

public enum ResourceClipboardOperation
{
    None,
    Copy,
    Cut,
}

public sealed record ResourceClipboardSnapshot(
    ResourceClipboardOperation Operation,
    IReadOnlyList<string> SourcePaths)
{
    public static ResourceClipboardSnapshot Empty { get; } =
        new(ResourceClipboardOperation.None, []);

    public bool HasItems =>
        Operation != ResourceClipboardOperation.None && SourcePaths.Count > 0;
}
