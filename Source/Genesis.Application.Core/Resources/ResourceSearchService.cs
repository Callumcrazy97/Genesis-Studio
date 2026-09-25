using System.Security;

namespace Genesis.Application.Core.Resources;

/// <summary>Where a Finder result matched the authored resource.</summary>
[Flags]
public enum ResourceSearchMatchKind
{
    None = 0,
    Name = 1,
    Path = 2,
    Content = 4,
    LibraryTag = 8,
}

/// <summary>The complete, UI-free query issued by Studio's resource Finder.</summary>
public sealed record ResourceSearchQuery
{
    public required string Term { get; init; }

    public required string ScopePath { get; init; }

    public bool IncludeSubfolders { get; init; } = true;

    public bool SearchContents { get; init; }

    /// <summary>An empty collection means every kind, including folders.</summary>
    public IReadOnlyCollection<ResourceKind> Kinds { get; init; } = [];

    public int MaximumResults { get; init; } = 500;
}

/// <summary>One resource result. Content matches identify the authored file and first matching line.</summary>
public sealed record ResourceSearchResult(
    ResourceItem Resource,
    ResourceSearchMatchKind MatchKind,
    string? MatchedRelativePath,
    int? LineNumber,
    string Preview)
{
    public string MatchLabel => MatchKind.HasFlag(ResourceSearchMatchKind.Content)
        ? LineNumber is int line
            ? $"{ResourceDisplayName.Format(MatchedRelativePath)}:{line}"
            : ResourceDisplayName.Format(MatchedRelativePath)
        : MatchKind.HasFlag(ResourceSearchMatchKind.Name)
            ? "Name"
            : MatchKind.HasFlag(ResourceSearchMatchKind.LibraryTag) ? "Library tags" : "Path";
}

/// <summary>Finder results plus bounded-scan diagnostics useful to the status surface.</summary>
public sealed record ResourceSearchResponse(
    IReadOnlyList<ResourceSearchResult> Results,
    int CandidateCount,
    int SkippedContentFiles,
    bool Truncated);

/// <summary>
/// Searches a resource-tree snapshot treated as immutable for the query. Content reads are bounded and the async entry point
/// runs them away from the UI thread; this layer deliberately has no WinForms dependency.
/// </summary>
public sealed class ResourceSearchService
{
    public const long MaximumContentFileBytes = 2 * 1024 * 1024;
    public const int MaximumContentFilesPerQuery = 8_192;
    public const long MaximumContentBytesPerQuery = 64 * 1024 * 1024;

    private static readonly HashSet<string> SearchableTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".pgsl", ".md", ".txt", ".hlsl", ".hlsli", ".glsl", ".vert", ".frag",
        ".yaml", ".yml", ".xml", ".csv",
    };

    public Task<ResourceSearchResponse> SearchAsync(
        ResourceItem assetsRoot,
        ResourceSearchQuery query,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Search(assetsRoot, query, cancellationToken), cancellationToken);

    public ResourceSearchResponse Search(
        ResourceItem assetsRoot,
        ResourceSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetsRoot);
        ArgumentNullException.ThrowIfNull(query);
        if (!assetsRoot.IsFolder)
        {
            throw new ArgumentException("The search root must be the Assets folder.", nameof(assetsRoot));
        }

        string term = query.Term.Trim();
        if (term.Length == 0)
        {
            return new ResourceSearchResponse([], 0, 0, false);
        }

        ResourceLibraryQuery textQuery = ResourceLibraryQuery.Parse(term);
        if (textQuery.Error is not null) throw new ArgumentException(textQuery.Error, nameof(query));
        string contentTerm = textQuery.ContentTerm;
        ResourceItem scope = ResolveScope(assetsRoot, query.ScopePath);
        HashSet<ResourceKind> kinds = new(query.Kinds);
        int maximumResults = Math.Clamp(query.MaximumResults, 1, 5_000);
        List<ResourceSearchResult> matches = [];
        int candidates = 0;
        int skippedContentFiles = 0;
        int remainingContentFiles = MaximumContentFilesPerQuery;
        long remainingContentBytes = MaximumContentBytesPerQuery;
        bool truncated = false;

        foreach (ResourceItem item in EnumerateScope(scope, query.IncludeSubfolders))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kinds.Count > 0 && !kinds.Contains(item.Kind))
            {
                continue;
            }

            candidates++;
            if (!textQuery.MatchesTags(item.LibraryTags) || (item.IsFolder && textQuery.HasTagFilters)) continue;
            ResourceSearchMatchKind metadataMatch = ResourceSearchMatchKind.None;
            if (textQuery.TextMatchesName(item.Name))
            {
                metadataMatch |= ResourceSearchMatchKind.Name;
            }

            if (contentTerm.Length > 0 && (Path.GetDirectoryName(item.RelativePath) ?? string.Empty).Contains(contentTerm, StringComparison.OrdinalIgnoreCase))
            {
                metadataMatch |= ResourceSearchMatchKind.Path;
            }

            if (textQuery.Matches(item.Name, item.LibraryTags, Path.GetDirectoryName(item.RelativePath) ?? string.Empty))
            {
                if (textQuery.HasTagFilters || textQuery.TextMatchesTag(item.LibraryTags))
                    metadataMatch |= ResourceSearchMatchKind.LibraryTag;
                if (metadataMatch == ResourceSearchMatchKind.None) metadataMatch = ResourceSearchMatchKind.Path;
            }
            else metadataMatch = ResourceSearchMatchKind.None;

            if (metadataMatch != ResourceSearchMatchKind.None)
            {
                ConsiderResult(matches, new ResourceSearchResult(
                    item,
                    metadataMatch,
                    null,
                    null,
                    metadataMatch == ResourceSearchMatchKind.LibraryTag
                        ? "Library tags: " + string.Join(", ", item.LibraryTags) : item.Name),
                    maximumResults,
                    ref truncated);
            }
            else if (query.SearchContents && !item.IsFolder && contentTerm.Length > 0)
            {
                ContentMatch? content = FindContentMatch(
                    assetsRoot.FullPath,
                    item,
                    contentTerm,
                    cancellationToken,
                    ref skippedContentFiles,
                    ref remainingContentFiles,
                    ref remainingContentBytes);
                if (content is not null)
                {
                    ConsiderResult(matches, new ResourceSearchResult(
                        item,
                        ResourceSearchMatchKind.Content,
                        content.RelativePath,
                        content.LineNumber,
                        content.Preview),
                        maximumResults,
                        ref truncated);
                }
            }
        }

        ResourceSearchResult[] ordered = matches
            .OrderBy(result => result, SearchResultComparer.Instance)
            .ToArray();
        return new ResourceSearchResponse(ordered, candidates, skippedContentFiles, truncated);
    }

    private static ResourceItem ResolveScope(ResourceItem root, string scopePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);
        string rootPath = NormalisePath(root.FullPath);
        string requested = NormalisePath(scopePath);
        if (!string.Equals(requested, rootPath, StringComparison.OrdinalIgnoreCase)
            && !requested.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Resource searches are restricted to the project Assets folder.");
        }

        ResourceItem? scope = Flatten(root).FirstOrDefault(item =>
            string.Equals(NormalisePath(item.FullPath), requested, StringComparison.OrdinalIgnoreCase));
        if (scope is null)
        {
            throw new DirectoryNotFoundException($"The resource search scope '{scopePath}' no longer exists.");
        }

        if (!scope.IsFolder)
        {
            string? parent = Path.GetDirectoryName(scope.FullPath);
            scope = parent is null
                ? root
                : Flatten(root).FirstOrDefault(item =>
                    item.IsFolder
                    && string.Equals(
                        NormalisePath(item.FullPath),
                        NormalisePath(parent),
                        StringComparison.OrdinalIgnoreCase)) ?? root;
        }

        return scope;
    }

    private static IEnumerable<ResourceItem> EnumerateScope(ResourceItem scope, bool includeSubfolders)
    {
        foreach (ResourceItem child in scope.Children)
        {
            yield return child;
            if (!includeSubfolders || !child.IsFolder)
            {
                continue;
            }

            foreach (ResourceItem descendant in FlattenChildren(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<ResourceItem> Flatten(ResourceItem root)
    {
        yield return root;
        foreach (ResourceItem child in FlattenChildren(root))
        {
            yield return child;
        }
    }

    private static IEnumerable<ResourceItem> FlattenChildren(ResourceItem root)
    {
        foreach (ResourceItem child in root.Children)
        {
            yield return child;
            foreach (ResourceItem descendant in FlattenChildren(child))
            {
                yield return descendant;
            }
        }
    }

    private static ContentMatch? FindContentMatch(
        string assetsRoot,
        ResourceItem resource,
        string term,
        CancellationToken cancellationToken,
        ref int skippedContentFiles,
        ref int remainingContentFiles,
        ref long remainingContentBytes)
    {
        foreach (string candidate in ContentCandidates(assetsRoot, resource, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? content = ReadBoundedText(
                candidate,
                cancellationToken,
                ref skippedContentFiles,
                ref remainingContentFiles,
                ref remainingContentBytes);
            if (content is null)
            {
                continue;
            }

            int index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            int lineNumber = 1;
            for (int offset = 0; offset < index; offset++)
            {
                if (content[offset] == '\n')
                {
                    lineNumber++;
                }
            }

            int start = content.LastIndexOf('\n', Math.Max(0, index - 1));
            start = start < 0 ? 0 : start + 1;
            int end = content.IndexOf('\n', index);
            end = end < 0 ? content.Length : end;
            string preview = CollapseWhitespace(content[start..end].Trim());
            if (preview.Length > 180)
            {
                preview = preview[..177] + "…";
            }

            return new ContentMatch(
                Path.GetRelativePath(assetsRoot, candidate).Replace('\\', '/'),
                lineNumber,
                preview);
        }

        return null;
    }

    private static IEnumerable<string> ContentCandidates(
        string assetsRoot,
        ResourceItem resource,
        CancellationToken cancellationToken)
    {
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        List<string> authoredPaths = [resource.FullPath];
        if (resource.Kind == ResourceKind.GameObject)
        {
            // The event directory is authored code owned by the Object but hidden from the Assets
            // tree. Generic associates are media/meta and do not belong in a text search; avoiding
            // ResourceAssociates.Find also avoids re-enumerating every sibling for every resource.
            authoredPaths.Add(ResourceAssociates.GetObjectEventDirectory(resource.FullPath));
        }

        foreach (string authoredPath in authoredPaths)
        {
            string fullAuthoredPath = Path.GetFullPath(authoredPath);
            if (!IsInside(fullAuthoredPath, assetsRoot))
            {
                continue;
            }

            foreach (string candidate in Expand(fullAuthoredPath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(candidate);
                if (!IsInside(fullPath, assetsRoot)
                    || !IsSearchableText(fullPath)
                    || !yielded.Add(fullPath))
                {
                    continue;
                }

                yield return fullPath;
            }
        }

        static IEnumerable<string> Expand(string path, CancellationToken cancellationToken)
        {
            if (File.Exists(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return [path];
            }

            if (!Directory.Exists(path))
            {
                return [];
            }

            List<string> files = [];
            try
            {
                EnumerationOptions options = new()
                {
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                };
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsSearchableText(file))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An associate may disappear while a watcher refresh is being processed. The main
                // resource remains searchable; this stale directory contributes no candidates.
                System.Diagnostics.Debug.WriteLine(
                    $"Finder skipped associate directory '{path}': {exception.Message}");
                return [];
            }

            return files;
        }
    }

    private static string? ReadBoundedText(
        string path,
        CancellationToken cancellationToken,
        ref int skippedContentFiles,
        ref int remainingContentFiles,
        ref long remainingContentBytes)
    {
        try
        {
            if (remainingContentFiles <= 0 || remainingContentBytes <= 0)
            {
                skippedContentFiles++;
                return null;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long initialLength = stream.Length;
            if (initialLength > MaximumContentFileBytes || initialLength > remainingContentBytes)
            {
                skippedContentFiles++;
                return null;
            }

            remainingContentFiles--;
            remainingContentBytes -= initialLength;

            byte[] bytes = new byte[(int)initialLength];
            int totalRead = 0;
            while (totalRead < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            // Decode only the byte snapshot observed after opening. If the file grows concurrently,
            // appended bytes are deliberately outside this bounded Finder query.
            using MemoryStream snapshot = new(bytes, 0, totalRead, writable: false);
            using StreamReader reader = new(snapshot, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Search is advisory and files can be replaced by editors/watchers between snapshot and
            // read. Count the skipped content rather than turning a stale hit into a Studio error.
            System.Diagnostics.Debug.WriteLine($"Finder skipped '{path}': {exception.Message}");
            skippedContentFiles++;
            return null;
        }
    }

    private static bool IsSearchableText(string path) =>
        !ResourceAssociates.IsHiddenImplementationFile(path)
        && (ResourceAssociates.IsDesignerVisibleResourcePath(path)
            || SearchableTextExtensions.Contains(Path.GetExtension(path)));

    private static bool IsInside(string path, string root)
    {
        string fullPath = NormalisePath(path);
        string fullRoot = NormalisePath(root);
        if (!fullPath.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A junction or symlink below Assets can be lexically inside while resolving elsewhere.
        // Treat the project Assets root as the authority, but reject any reparse-point hop beneath it.
        for (string? current = fullPath;
             current is not null && !string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalisePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int Rank(ResourceSearchMatchKind match) =>
        match.HasFlag(ResourceSearchMatchKind.Name)
            ? 0
            : match.HasFlag(ResourceSearchMatchKind.LibraryTag) ? 1
            : match.HasFlag(ResourceSearchMatchKind.Path) ? 2 : 3;

    private static void ConsiderResult(
        List<ResourceSearchResult> matches,
        ResourceSearchResult candidate,
        int maximumResults,
        ref bool truncated)
    {
        if (matches.Count < maximumResults)
        {
            matches.Add(candidate);
            return;
        }

        truncated = true;
        int worstIndex = 0;
        for (int index = 1; index < matches.Count; index++)
        {
            if (SearchResultComparer.Instance.Compare(matches[index], matches[worstIndex]) > 0)
            {
                worstIndex = index;
            }
        }

        if (SearchResultComparer.Instance.Compare(candidate, matches[worstIndex]) < 0)
        {
            matches[worstIndex] = candidate;
        }
    }

    private sealed class SearchResultComparer : IComparer<ResourceSearchResult>
    {
        public static SearchResultComparer Instance { get; } = new();

        public int Compare(ResourceSearchResult? left, ResourceSearchResult? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            int rank = Rank(left.MatchKind).CompareTo(Rank(right.MatchKind));
            if (rank != 0)
            {
                return rank;
            }

            int name = StringComparer.OrdinalIgnoreCase.Compare(
                left.Resource.Name,
                right.Resource.Name);
            return name != 0
                ? name
                : StringComparer.OrdinalIgnoreCase.Compare(
                    left.Resource.RelativePath,
                    right.Resource.RelativePath);
        }
    }

    private sealed record ContentMatch(string RelativePath, int LineNumber, string Preview);
}
