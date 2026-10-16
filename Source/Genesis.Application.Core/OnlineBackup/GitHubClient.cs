using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Application.Core.Backup;

/// <summary>
/// Async GitHub REST + Git Data API client. Everything the app does goes through here;
/// there is no dependency on git.exe or the GitHub CLI.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    public const string ApiRoot = "https://api.github.com";
    private const string ApiVersion = "2022-11-28";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private string? _token;

    public GhUser? CurrentUser { get; private set; }
    public IReadOnlyList<string> TokenScopes { get; private set; } = Array.Empty<string>();
    public bool IsSignedIn => !string.IsNullOrEmpty(_token) && CurrentUser is not null;

    public event Action? AuthenticationChanged;

    public GitHubClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GenesisStudio/0.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
    }

    // ---------------------------------------------------------------- authentication

    public void SetToken(string? token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (_token is null)
        {
            CurrentUser = null;
            TokenScopes = Array.Empty<string>();
        }
        AuthenticationChanged?.Invoke();
    }

    public string? Token => _token;

    /// <summary>Validates the current token and caches the account plus its OAuth scopes.</summary>
    public async Task<GhUser> SignInAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Get, "/user"), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var user = Deserialise<GhUser>(body) ?? throw new GitHubApiException(500, "GitHub returned an empty user record.");

        if (response.Headers.TryGetValues("X-OAuth-Scopes", out var scopeValues))
        {
            TokenScopes = scopeValues.FirstOrDefault()?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>();
        }
        else
        {
            // Fine-grained tokens do not advertise classic scopes.
            TokenScopes = Array.Empty<string>();
        }

        CurrentUser = user;
        AuthenticationChanged?.Invoke();
        return user;
    }

    public void SignOut()
    {
        SetToken(null);
        CredentialStore.Clear();
    }

    public async Task<GhRateLimit?> GetRateLimitAsync(CancellationToken ct = default)
    {
        try { return await GetAsync<GhRateLimit>("/rate_limit", ct).ConfigureAwait(false); }
        catch (Exception ex) { Logger.Debug("Rate limit lookup failed: " + ex.Message); return null; }
    }

    // ---------------------------------------------------------------- repositories

    public async Task<List<GhRepo>> ListRepositoriesAsync(CancellationToken ct = default, Action<int>? onPage = null)
    {
        var result = new List<GhRepo>();
        var url = "/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member";
        var page = 0;

        while (url is not null)
        {
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var batch = Deserialise<List<GhRepo>>(body) ?? new List<GhRepo>();
            result.AddRange(batch);
            onPage?.Invoke(++page);
            url = NextLink(response);
            if (page > 50) break; // 5000 repositories is well past any sane limit
        }

        return result;
    }

    public Task<GhRepo> GetRepositoryAsync(string owner, string repo, CancellationToken ct = default) =>
        GetRequiredAsync<GhRepo>($"/repos/{Esc(owner)}/{Esc(repo)}", ct);

    public Task<GhRepo> CreateRepositoryAsync(string name, string? description, bool isPrivate, bool autoInit,
        string? organisation = null, CancellationToken ct = default)
    {
        var path = string.IsNullOrWhiteSpace(organisation) ? "/user/repos" : $"/orgs/{Esc(organisation!)}/repos";
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["private"] = isPrivate,
            ["auto_init"] = autoInit
        };
        return PostRequiredAsync<GhRepo>(path, payload, ct);
    }

    public Task<GhRepo> UpdateRepositoryAsync(string owner, string repo, string? newName, string? description,
        bool? isPrivate, string? defaultBranch, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(newName)) payload["name"] = newName;
        if (description is not null) payload["description"] = description;
        if (isPrivate.HasValue) payload["private"] = isPrivate.Value;
        if (!string.IsNullOrWhiteSpace(defaultBranch)) payload["default_branch"] = defaultBranch;

        return SendJsonRequiredAsync<GhRepo>(HttpMethod.Patch, $"/repos/{Esc(owner)}/{Esc(repo)}", payload, ct);
    }

    public async Task DeleteRepositoryAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            using var _ = await SendAsync(() => Request(HttpMethod.Delete, $"/repos/{Esc(owner)}/{Esc(repo)}"), ct)
                .ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.IsForbidden)
        {
            throw new GitHubApiException(ex.StatusCode,
                "Deleting a repository needs the 'delete_repo' scope. Add it to your token (classic tokens: tick delete_repo; " +
                "fine-grained tokens: Administration = Read and write), then sign in again.",
                ex.DocumentationUrl, ex.Errors);
        }
    }

    public async Task<List<GhBranch>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default)
    {
        var result = new List<GhBranch>();
        var url = $"/repos/{Esc(owner)}/{Esc(repo)}/branches?per_page=100";
        while (url is not null)
        {
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            result.AddRange(Deserialise<List<GhBranch>>(body) ?? new List<GhBranch>());
            url = NextLink(response);
        }
        return result;
    }

    // ---------------------------------------------------------------- git data

    /// <summary>Head commit SHA of a branch, or null when the branch (or repository history) does not exist yet.</summary>
    public async Task<string?> TryGetBranchHeadAsync(string owner, string repo, string branch, CancellationToken ct = default)
    {
        try
        {
            var reference = await GetRequiredAsync<GhRef>(
                $"/repos/{Esc(owner)}/{Esc(repo)}/git/ref/heads/{EscPath(branch)}", ct).ConfigureAwait(false);
            return reference.Object?.Sha;
        }
        catch (GitHubApiException ex) when (ex.IsNotFound)
        {
            return null;
        }
        catch (GitHubApiException ex) when (ex.IsForbidden && ex.Message.Contains("empty", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public Task<GhCommit> GetCommitAsync(string owner, string repo, string sha, CancellationToken ct = default) =>
        GetRequiredAsync<GhCommit>($"/repos/{Esc(owner)}/{Esc(repo)}/git/commits/{Esc(sha)}", ct);

    public Task<GhTree> GetTreeAsync(string owner, string repo, string sha, bool recursive, CancellationToken ct = default) =>
        GetRequiredAsync<GhTree>(
            $"/repos/{Esc(owner)}/{Esc(repo)}/git/trees/{Esc(sha)}" + (recursive ? "?recursive=1" : ""), ct);

    /// <summary>Uploads a blob and returns its SHA. Content is base64-wrapped as the API requires.</summary>
    public async Task<string> CreateBlobAsync(string owner, string repo, byte[] content, IByteMeter? meter,
        CancellationToken ct = default)
    {
        var payload = new StringBuilder(((content.Length + 2) / 3 * 4) + 64);
        payload.Append("{\"content\":\"").Append(Convert.ToBase64String(content)).Append("\",\"encoding\":\"base64\"}");
        var bytes = Encoding.UTF8.GetBytes(payload.ToString());

        using var response = await SendAsync(
            () =>
            {
                var request = Request(HttpMethod.Post, $"/repos/{Esc(owner)}/{Esc(repo)}/git/blobs");
                request.Content = new MeteredContent(bytes, "application/json", meter);
                return request;
            }, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var created = Deserialise<GhTreeEntry>(body);
        if (string.IsNullOrEmpty(created?.Sha))
            throw new GitHubApiException(500, "GitHub accepted the blob but returned no SHA.");
        return created!.Sha!;
    }

    /// <summary>Streams a blob straight to disk, metering as it goes.</summary>
    public async Task DownloadBlobAsync(string owner, string repo, string sha, string destinationPath,
        IByteMeter? meter, Action<long>? onBytes, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = Request(HttpMethod.Get, $"/repos/{Esc(owner)}/{Esc(repo)}/git/blobs/{Esc(sha)}");
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("application/vnd.github.raw");
                return request;
            }, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".autosync-part";

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                meter?.AddWireBytes(read);
                onBytes?.Invoke(read);
            }
        }

        File.Move(temp, destinationPath, overwrite: true);
    }

    public async Task<string> CreateTreeAsync(string owner, string repo, IReadOnlyList<GhTreeEntry> entries,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tree"] = entries.Select(e => new Dictionary<string, object?>
            {
                ["path"] = e.Path,
                ["mode"] = e.Mode,
                ["type"] = e.Type,
                ["sha"] = e.Sha
            }).ToList()
        };

        var tree = await PostRequiredAsync<GhTree>($"/repos/{Esc(owner)}/{Esc(repo)}/git/trees", payload, ct)
            .ConfigureAwait(false);
        return tree.Sha;
    }

    /// <summary>
    /// A tree made from <paramref name="baseTree"/> plus <paramref name="entries"/>: only the changed
    /// paths are sent, and an entry with a null sha deletes that path. Added for Genesis's backup,
    /// which shares one repository between projects and must leave the others untouched.
    /// </summary>
    public async Task<string> CreateTreeAsync(string owner, string repo, string? baseTree, IReadOnlyList<GhTreeEntry> entries,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tree"] = entries.Select(e => new Dictionary<string, object?>
            {
                ["path"] = e.Path,
                ["mode"] = e.Mode,
                ["type"] = e.Type,
                ["sha"] = e.Sha
            }).ToList()
        };
        if (!string.IsNullOrEmpty(baseTree)) payload["base_tree"] = baseTree;

        var tree = await PostRequiredAsync<GhTree>($"/repos/{Esc(owner)}/{Esc(repo)}/git/trees", payload, ct)
            .ConfigureAwait(false);
        return tree.Sha;
    }

    public async Task<string> CreateCommitAsync(string owner, string repo, string message, string treeSha,
        IReadOnlyList<string> parents, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["tree"] = treeSha,
            ["parents"] = parents
        };

        var commit = await PostRequiredAsync<GhCommit>($"/repos/{Esc(owner)}/{Esc(repo)}/git/commits", payload, ct)
            .ConfigureAwait(false);
        return commit.Sha;
    }

    public async Task UpdateBranchAsync(string owner, string repo, string branch, string commitSha, bool force,
        CancellationToken ct = default)
    {
        var refPath = $"/repos/{Esc(owner)}/{Esc(repo)}/git/refs/heads/{EscPath(branch)}";
        var payload = new Dictionary<string, object?> { ["sha"] = commitSha, ["force"] = force };

        try
        {
            using var _ = await SendJsonAsync(HttpMethod.Patch, refPath, payload, ct).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.IsNotFound)
        {
            var create = new Dictionary<string, object?> { ["ref"] = $"refs/heads/{branch}", ["sha"] = commitSha };
            using var _ = await SendJsonAsync(HttpMethod.Post, $"/repos/{Esc(owner)}/{Esc(repo)}/git/refs", create, ct)
                .ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- releases

    public async Task<List<GhRelease>> ListReleasesAsync(string owner, string repo, CancellationToken ct = default)
    {
        var result = new List<GhRelease>();
        var url = $"/repos/{Esc(owner)}/{Esc(repo)}/releases?per_page=100";
        while (url is not null)
        {
            using var response = await SendAsync(() => Request(HttpMethod.Get, url), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            result.AddRange(Deserialise<List<GhRelease>>(body) ?? new List<GhRelease>());
            url = NextLink(response);
        }
        return result;
    }

    public Task<GhRelease> CreateReleaseAsync(string owner, string repo, string tag, string? name, string? notes,
        string? targetCommitish, bool prerelease, bool draft, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["name"] = string.IsNullOrWhiteSpace(name) ? tag : name,
            ["body"] = notes ?? "",
            ["prerelease"] = prerelease,
            ["draft"] = draft
        };
        if (!string.IsNullOrWhiteSpace(targetCommitish)) payload["target_commitish"] = targetCommitish;

        return PostRequiredAsync<GhRelease>($"/repos/{Esc(owner)}/{Esc(repo)}/releases", payload, ct);
    }

    public Task<GhRelease> UpdateReleaseAsync(string owner, string repo, long releaseId, string? tag, string? name,
        string? notes, bool? prerelease, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(tag)) payload["tag_name"] = tag;
        if (name is not null) payload["name"] = name;
        if (notes is not null) payload["body"] = notes;
        if (prerelease.HasValue) payload["prerelease"] = prerelease.Value;

        return SendJsonRequiredAsync<GhRelease>(HttpMethod.Patch,
            $"/repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", payload, ct);
    }

    public async Task DeleteReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(
            () => Request(HttpMethod.Delete, $"/repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}"), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Downloads an arbitrary GitHub URL (release zipball, tarball) to a file with metering.</summary>
    public async Task DownloadToFileAsync(string url, string destinationPath, IByteMeter? meter,
        Action<long>? onBytes, Action<long>? onTotalKnown, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Get, url), ct, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        if (response.Content.Headers.ContentLength is long total && total > 0) onTotalKnown?.Invoke(total);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".autosync-part";

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                meter?.AddWireBytes(read);
                onBytes?.Invoke(read);
            }
        }

        File.Move(temp, destinationPath, overwrite: true);
    }

    // ---------------------------------------------------------------- plumbing

    private HttpRequestMessage Request(HttpMethod method, string pathOrUrl)
    {
        var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : ApiRoot + pathOrUrl;
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken ct)
    {
        var value = await GetAsync<T>(path, ct).ConfigureAwait(false);
        return value ?? throw new GitHubApiException(500, $"GitHub returned an empty {typeof(T).Name} response.");
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => Request(HttpMethod.Get, path), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Deserialise<T>(body);
    }

    private Task<T> PostRequiredAsync<T>(string path, object payload, CancellationToken ct) =>
        SendJsonRequiredAsync<T>(HttpMethod.Post, path, payload, ct);

    private async Task<T> SendJsonRequiredAsync<T>(HttpMethod method, string path, object payload, CancellationToken ct)
    {
        using var response = await SendJsonAsync(method, path, payload, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Deserialise<T>(body) ?? throw new GitHubApiException(500, $"GitHub returned an empty {typeof(T).Name} response.");
    }

    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return SendAsync(() =>
        {
            var request = Request(method, path);
            request.Content = new MeteredContent(bytes, "application/json", null);
            return request;
        }, ct);
    }

    /// <summary>
    /// Single funnel for every request: applies rate-limit / secondary-limit / transient retries and
    /// converts failures into a <see cref="GitHubApiException"/> carrying GitHub's own message.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            using (var request = factory())
            {
                try
                {
                    response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt >= maxAttempts) throw new GitHubApiException(408, "The request to GitHub timed out.");
                    await Task.Delay(BackoffMs(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= maxAttempts)
                        throw new GitHubApiException(0, "Could not reach github.com: " + ex.Message);
                    await Task.Delay(BackoffMs(attempt), ct).ConfigureAwait(false);
                    continue;
                }
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var retryAfter = GetRetrySeconds(response);
            response.Dispose();

            var retryable = status is 429 or 500 or 502 or 503 or 504 ||
                            (status == 403 && retryAfter is not null);

            if (retryable && attempt < maxAttempts)
            {
                var delay = retryAfter is { } seconds ? (int)Math.Min(seconds * 1000, 60_000) : BackoffMs(attempt);
                Logger.Warn($"GitHub returned HTTP {status}; retrying in {delay / 1000.0:0.#}s (attempt {attempt} of {maxAttempts - 1}).");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            throw BuildException(status, body);
        }
    }

    private static int BackoffMs(int attempt) => (int)Math.Min(1000 * Math.Pow(2, attempt - 1), 15_000);

    private static double? GetRetrySeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values) &&
            double.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return seconds;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
            remaining.FirstOrDefault() == "0" &&
            response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
            long.TryParse(reset.FirstOrDefault(), out var epoch))
        {
            var wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return Math.Min(wait.TotalSeconds + 1, 60);
        }

        return null;
    }

    private static GitHubApiException BuildException(int status, string body)
    {
        var message = status switch
        {
            401 => "Your GitHub token is missing, expired or revoked. Sign in again.",
            403 => "GitHub refused the request (permission or rate limit).",
            404 => "Not found. The repository, branch or release does not exist, or your token cannot see it.",
            409 => "Conflict. The branch moved on GitHub since this operation started.",
            422 => "GitHub rejected the request as invalid.",
            _ => $"Request failed with status {status}."
        };

        var errors = new List<string>();
        string? docUrl = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var text = m.GetString();
                if (!string.IsNullOrWhiteSpace(text)) message = text!;
            }

            if (root.TryGetProperty("documentation_url", out var d) && d.ValueKind == JsonValueKind.String)
                docUrl = d.GetString();

            if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) { errors.Add(item.GetString() ?? ""); continue; }

                    var field = item.TryGetProperty("field", out var f) ? f.GetString() : null;
                    var code = item.TryGetProperty("code", out var c) ? c.GetString() : null;
                    var detail = item.TryGetProperty("message", out var im) ? im.GetString() : null;
                    errors.Add(string.Join(" ", new[] { field, code, detail }.Where(x => !string.IsNullOrWhiteSpace(x))));
                }
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(body) && body.Length < 400) message += " " + body.Trim();
        }

        return new GitHubApiException(status, message, docUrl, errors);
    }

    private static T? Deserialise<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException ex) { throw new GitHubApiException(500, "GitHub sent a response this app could not parse: " + ex.Message); }
    }

    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;

        foreach (var header in values)
        {
            foreach (var part in header.Split(','))
            {
                var segments = part.Split(';');
                if (segments.Length < 2) continue;
                if (!segments[1].Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)) continue;
                return segments[0].Trim().Trim('<', '>');
            }
        }

        return null;
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    /// <summary>Branch names may legitimately contain '/', which must survive escaping.</summary>
    private static string EscPath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();
}
