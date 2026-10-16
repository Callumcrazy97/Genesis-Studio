using System.Text.Json.Serialization;

namespace Genesis.Application.Core.Backup;

public sealed class GhUser
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

public sealed class GhRepo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [JsonPropertyName("private")] public bool Private { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("default_branch")] public string DefaultBranch { get; set; } = "main";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
    [JsonPropertyName("pushed_at")] public DateTimeOffset? PushedAt { get; set; }
    [JsonPropertyName("size")] public long SizeKb { get; set; }
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("owner")] public GhUser? Owner { get; set; }

    [JsonIgnore] public string OwnerLogin => Owner?.Login ?? (FullName.Contains('/') ? FullName.Split('/')[0] : "");
}

public sealed class GhRefObject
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public sealed class GhRef
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("object")] public GhRefObject? Object { get; set; }
}

public sealed class GhCommitRef
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
}

public sealed class GhCommit
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("tree")] public GhCommitRef? Tree { get; set; }
    [JsonPropertyName("parents")] public List<GhCommitRef>? Parents { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

public sealed class GhTreeEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "100644";
    [JsonPropertyName("type")] public string Type { get; set; } = "blob";
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class GhTree
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    [JsonPropertyName("tree")] public List<GhTreeEntry> Tree { get; set; } = new();
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public sealed class GhRelease
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("zipball_url")] public string? ZipballUrl { get; set; }
    [JsonPropertyName("tarball_url")] public string? TarballUrl { get; set; }
    [JsonPropertyName("target_commitish")] public string? TargetCommitish { get; set; }
}

public sealed class GhBranch
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("commit")] public GhCommitRef? Commit { get; set; }
}

public sealed class GhRateLimitCore
{
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("remaining")] public int Remaining { get; set; }
    [JsonPropertyName("reset")] public long Reset { get; set; }
}

public sealed class GhRateLimitResources
{
    [JsonPropertyName("core")] public GhRateLimitCore? Core { get; set; }
}

public sealed class GhRateLimit
{
    [JsonPropertyName("resources")] public GhRateLimitResources? Resources { get; set; }
}

/// <summary>A GitHub API call that came back with a non-success status.</summary>
public sealed class GitHubApiException : Exception
{
    public int StatusCode { get; }
    public string? DocumentationUrl { get; }
    public IReadOnlyList<string> Errors { get; }

    public GitHubApiException(int statusCode, string message, string? documentationUrl = null, IReadOnlyList<string>? errors = null)
        : base(message)
    {
        StatusCode = statusCode;
        DocumentationUrl = documentationUrl;
        Errors = errors ?? Array.Empty<string>();
    }

    public bool IsNotFound => StatusCode == 404;
    public bool IsUnauthorised => StatusCode is 401;
    public bool IsForbidden => StatusCode is 403;

    public string FullText
    {
        get
        {
            var text = $"GitHub returned HTTP {StatusCode}: {Message}";
            if (Errors.Count > 0) text += Environment.NewLine + string.Join(Environment.NewLine, Errors.Select(e => "  - " + e));
            if (!string.IsNullOrWhiteSpace(DocumentationUrl)) text += Environment.NewLine + "See: " + DocumentationUrl;
            return text;
        }
    }
}
